<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:fn="http://www.w3.org/2005/xpath-functions" xmlns:math="http://www.w3.org/2005/xpath-functions/math" xmlns:array="http://www.w3.org/2005/xpath-functions/array" xmlns:map="http://www.w3.org/2005/xpath-functions/map" xmlns:xhtml="http://www.w3.org/1999/xhtml" xmlns:err="http://www.w3.org/2005/xqt-errors" exclude-result-prefixes="array fn map math xhtml xs err" version="3.0" xmlns:bf="urn:BimHouse:XslFunctions" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'>

	<xsl:variable name="Spaces" select="/*"/>

	<xsl:function name="bf:InstanceOf" as="xs:boolean">
		<xsl:param name="Element"/>
		<xsl:param name="TypeNamespace"/>
		<xsl:param name="TypeName"/>
		<xsl:choose>
			<xsl:when test="$Element/@xsi:type">
				<xsl:variable name="ElementTypeQName" select="resolve-QName(string($Element/@xsi:type),$Spaces)" as="xs:QName"/>
				<xsl:variable name="ElementTypeNamespace" select="string(namespace-uri-from-QName($ElementTypeQName))"/>
				<xsl:variable name="ElementTypeName" select="string(local-name-from-QName($ElementTypeQName))"/>
				<xsl:choose>
					<xsl:when test="$ElementTypeNamespace = $TypeNamespace and starts-with($ElementTypeName,$TypeName)">
						<xsl:value-of select="true()"/>
					</xsl:when>
					<xsl:otherwise>
						<xsl:value-of select='false()'/>
					</xsl:otherwise>
				</xsl:choose>
			</xsl:when>
			<xsl:otherwise>
				<xsl:value-of select='false()'/>
			</xsl:otherwise>
		</xsl:choose>
		<xsl:if test="$Element/@xsi:type">
		</xsl:if>
	</xsl:function>

	<xsl:function name="bf:SuperTypeOf" as="xs:boolean">
		<xsl:param name="Element"/>
		<xsl:param name="TypeNamespace"/>
		<xsl:param name="TypeName"/>
		<xsl:choose>
			<xsl:when test="$Element/@xsi:type">
				<xsl:variable name="ElementTypeQName" select="resolve-QName(string($Element/@xsi:type),$Spaces)" as="xs:QName"/>
				<xsl:variable name="ElementTypeNamespace" select="string(namespace-uri-from-QName($ElementTypeQName))"/>
				<xsl:variable name="ElementTypeName" select="string(local-name-from-QName($ElementTypeQName))"/>
				<xsl:choose>
					<xsl:when test="$ElementTypeNamespace = $TypeNamespace and starts-with($TypeName,$ElementTypeName)">
						<xsl:value-of select="true()"/>
					</xsl:when>
					<xsl:otherwise>
						<xsl:value-of select='false()'/>
					</xsl:otherwise>
				</xsl:choose>
			</xsl:when>
			<xsl:otherwise>
				<xsl:value-of select='false()'/>
			</xsl:otherwise>
		</xsl:choose>
		<xsl:if test="$Element/@xsi:type">
		</xsl:if>
	</xsl:function>
	
	<xsl:function name="bf:CheckAndCorrectUri" as="xs:anyURI">
		<xsl:param name="SourceURI" required="yes" as="xs:string"/>
		<xsl:param name="BaseUri" as="xs:anyURI"></xsl:param>
		<xsl:variable name="NormUri" as="xs:anyURI">
			<xsl:choose>
				<xsl:when test="fn:matches($SourceURI,'^[a-z]+://.*')">
					<xsl:value-of select="xs:anyURI($SourceURI)"/>
				</xsl:when>
				<xsl:otherwise>
					<xsl:variable name="AlmoustURI" select="fn:iri-to-uri(fn:replace(fn:replace($SourceURI,'\\','/'),'#','%23'))"/>
					<xsl:variable name="NewURI" as="xs:anyURI">
						<xsl:choose>
							<xsl:when test="not(fn:starts-with($SourceURI,'.'))">
								<xsl:value-of select="xs:anyURI(fn:concat('file:///',$AlmoustURI))"/>
							</xsl:when>
							<xsl:otherwise><xsl:value-of select="xs:anyURI($AlmoustURI)"/></xsl:otherwise>
						</xsl:choose>
					</xsl:variable>
					<xsl:value-of select="xs:anyURI($NewURI)"/>
				</xsl:otherwise>
			</xsl:choose>
		</xsl:variable>
		<xsl:variable name="ResolvedURI" as="xs:anyURI">
			<xsl:choose>
				<xsl:when test="$BaseUri"><xsl:value-of select="fn:resolve-uri($NormUri,$BaseUri)"/></xsl:when>
				<xsl:otherwise><xsl:value-of select="$NormUri"/></xsl:otherwise>
			</xsl:choose>
		</xsl:variable>
		<xsl:try>
			<xsl:if test="$ResolvedURI and fn:document($ResolvedURI)">
				<xsl:copy-of select="$ResolvedURI"/>
			</xsl:if>
			<xsl:catch errors="*">
				<xsl:message terminate="yes">
					<xsl:text>Документ: </xsl:text><xsl:value-of select="$SourceURI"/><xsl:text> не найден</xsl:text>
					<xsl:if test="$BaseUri">
						<xsl:text>&#xA;        относительно: </xsl:text><xsl:value-of select="$BaseUri"/>
					</xsl:if>
				</xsl:message>
			</xsl:catch>
		</xsl:try>
	</xsl:function>


</xsl:stylesheet>
